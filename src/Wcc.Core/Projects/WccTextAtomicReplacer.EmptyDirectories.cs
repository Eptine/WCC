using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Wcc.Core.Projects;

public sealed partial class WccTextAtomicReplacer
{
    // These probes observe existing directory identities through the same guarded route
    // capture used by file Save. No directory is created, enumerated recursively, or deleted here.
    internal static IReadOnlyList<WccTextDiskFileAbsence> CaptureDirectoryRemovalRoutes(string projectRoot, IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        if (directories.Count > 2 || directories.Distinct(DiskPathComparer).Count() != directories.Count)
            throw new InvalidOperationException("A chapter cleanup requires at most two distinct exact managed directories.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var result = new List<WccTextDiskFileAbsence>();
        foreach (var candidate in directories)
        {
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
            var parts = relative.Split('/');
            if (parts.Length is not (2 or 3) || parts[0] != "chapters" ||
                Creation.WccCampaignCreationTokenPolicy.ValidateAddonId(parts[1]) is not null || parts.Length == 3 && parts[2] != "scenarios")
                throw new InvalidOperationException("Empty-directory cleanup is limited to the removed chapter's exact managed folders.");
            var probe = Path.Combine(directory, ".wcc-directory-cleanup-" + Guid.NewGuid().ToString("N"));
            var capture = WccTextDiskRevisionCapture.CaptureAbsence(root, probe);
            if (capture.IsCaptured && capture.Absence is { } observed) { result.Add(observed); continue; }
            // Already missing folders need no cleanup. A file, link, changed route or
            // unavailable parent never becomes permission to remove a different object.
            if (WccTextDiskRevisionCapture.CaptureCreationAbsence(root, directory).IsCaptured) continue;
            throw new InvalidOperationException("The chapter directory route could not be captured: " + capture.ErrorMessage);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    internal static WccTextAtomicBatchReplaceResult CleanupEmptyReviewedDirectories(WccTextAtomicBatchReplaceResult saved,
        IReadOnlyList<WccTextDiskFileAbsence> directories)
    {
        if (!saved.IsSuccess || directories.Count == 0) return saved;
        var notes = new List<string>(); var removed = new List<string>(); var warning = false;
        foreach (var directory in directories.OrderByDescending(item => item.SourceParentPath.Length).ThenBy(item => item.SourceParentPath, DiskPathComparer))
        {
            try
            {
                var outcome = RemoveEmptyReviewedDirectory(directory);
                if (outcome.Removed) removed.Add(directory.SourceParentPath);
                else if (outcome.Note is { } note)
                { notes.Add(directory.SourceParentPath + ": " + note); warning |= outcome.Warning; }
            }
            catch (Exception issue) when (issue is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            { notes.Add(directory.SourceParentPath + ": the folder was retained: " + issue.Message); warning = true; }
        }
        return new(warning ? WccTextAtomicBatchReplaceStatus.SucceededWithCleanupWarning : saved.Status,
            saved.Diagnostic + (notes.Count == 0 ? "" : " Chapter folder cleanup: " + string.Join(" ", notes)), saved.Files)
        { CreatedDirectories = saved.CreatedDirectories, RemovedDirectories = Array.AsReadOnly(removed.ToArray()) };
    }

    private static (bool Removed, string? Note, bool Warning) RemoveEmptyReviewedDirectory(WccTextDiskFileAbsence expected)
    {
        var current = WccTextDiskRevisionCapture.CaptureAbsence(expected.ProjectRootPath, expected.SourcePath);
        if (!current.IsCaptured || current.Absence is null || !expected.IsSameAbsenceAs(current.Absence))
            return (false, "the directory identity or route changed; it was retained.", true);
        var path = expected.SourceParentPath;
        var parent = Path.GetDirectoryName(path)!;
        var parentObservation = WccTextDiskRevisionCapture.CaptureAbsence(expected.ProjectRootPath,
            Path.Combine(parent, ".wcc-directory-cleanup-parent-" + Guid.NewGuid().ToString("N")));
        if (!parentObservation.IsCaptured || parentObservation.Absence is null)
            return (false, "the parent route could not be verified; the folder was retained.", true);
        using var parentLease = WccTextDiskRevisionCapture.AcquireAbsenceRouteLease(parentObservation.Absence);
        if (OperatingSystem.IsLinux())
        {
            using var target = WccLinuxDiskNative.OpenAt(parentLease.ParentHandle, Path.GetFileName(path),
                WccLinuxDiskNative.ReadOnly | WccLinuxDiskNative.Directory | WccLinuxDiskNative.NoFollow | WccLinuxDiskNative.CloseOnExec);
            var identity = WccLinuxDiskNative.ReadSnapshot(target);
            if (!identity.IsDirectory || identity.Identity != expected.SourceParentIdentity)
                return (false, "the opened directory differs from the reviewed directory; it was retained.", true);
            if (WccLinuxDiskNative.DirectoryNames(target).Count != 0) return NonemptyDirectory();
            parentLease.VerifyCurrentRoute();
            var final = WccTextDiskRevisionCapture.CaptureAbsence(expected.ProjectRootPath, expected.SourcePath);
            if (!final.IsCaptured || final.Absence is null || !expected.IsSameAbsenceAs(final.Absence))
                return (false, "the directory route changed before cleanup; it was retained.", true);
            // Linux removes by parent descriptor and name. AT_REMOVEDIR makes the
            // kernel refuse any nonempty directory, including a last-moment arrival.
            // It cannot bind unlink to this exact inode atomically; a replacement
            // empty inode is the residual race, but file contents cannot be removed.
            var result = RemoveDirectoryAtLinux(WccLinuxDiskNative.Descriptor(parentLease.ParentHandle), Path.GetFileName(path), 0x200);
            var error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
            GC.KeepAlive(target);
            if (error is 39 or 17) return NonemptyDirectory(); // ENOTEMPTY/EEXIST.
            if (error != 0) return (false, "the empty folder could not be removed: " + new Win32Exception(error).Message, true);
            FlushAndVerifyLinuxRoute(parentLease);
        }
        else
        {
            // The parent lease pins ancestors. This same target handle verifies
            // the directory and asks the kernel for empty-only deletion.
            using var target = CreateFileWindows(path, 1 | DeleteAccess, FileShareRead | 2,
                IntPtr.Zero, OpenExisting, 0x02000000 | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (target.IsInvalid) return (false, "the directory could not be opened for cleanup: " + new Win32Exception(Marshal.GetLastWin32Error()).Message, true);
            if (!GetFileInformationByHandleWindows(target, out var info) ||
                (info.FileAttributes & FileAttributeDirectory) == 0 || (info.FileAttributes & 0x400) != 0 ||
                WccTextDiskRevisionCapture.ReadHandleIdentityForVerification(target) != expected.SourceParentIdentity)
                return (false, "the opened directory identity or type changed; it was retained.", true);
            if (Directory.EnumerateFileSystemEntries(path).Any()) return NonemptyDirectory();
            parentLease.VerifyCurrentRoute();
            var disposition = new FileDispositionInformation { DeleteFile = 1 };
            if (!SetFileInformationByHandleWindows(target, FileDispositionInfo, ref disposition, (uint)Marshal.SizeOf<FileDispositionInformation>()))
            {
                var error = Marshal.GetLastWin32Error();
                return error == 145 ? NonemptyDirectory() : (false, "the empty folder could not be removed: " + new Win32Exception(error).Message, true);
            }
        }
        // A later foreign replacement is left alone; never retry deletion by name.
        return WccTextDiskRevisionCapture.CaptureAbsence(expected.ProjectRootPath, path).IsCaptured
            ? (true, null, false) : (false, "the name was recreated after cleanup; the replacement was left alone.", true);
    }
    private static (bool Removed, string? Note, bool Warning) NonemptyDirectory() =>
        (false, "the folder is not empty; remaining user files or recovery copies were preserved.", false);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int RemoveDirectoryAtLinux(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
}
