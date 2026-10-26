using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Wcc.Core.Projects;

public sealed partial class WccTextAtomicReplacer
{
    // Deletion participates in the same staged review, commit guard and recovery
    // reporting as replacements. Never delete a pathname after the batch returns.
    private static int DeleteNativeChecked(MixedBatchStage stage)
    {
        var original = stage.Mutation.Deletion!.Expected;
        stage.Lease!.VerifyCurrentRoute();
        if (OperatingSystem.IsLinux())
        {
            using var source = OpenPublicationFileLinux(stage.Lease, original.SourcePath);
            using var sourceLock = WccLinuxDiskNative.AcquireExclusiveLock(source);
            var current = CapturePublicationFileLinux(source);
            if (current.Metadata.Identity != original.SourceIdentity || current.Metadata.LinkCount != 1
                || current.Metadata.Length != original.RawByteLength || current.Hash != original.RawByteSha256)
                throw new IOException("The deletion source no longer matches its reviewed inode and bytes.");
            stage.Lease.VerifyCurrentRoute();
            var result = RenameAtLinux(WccLinuxDiskNative.Descriptor(stage.Lease.ParentHandle), Path.GetFileName(original.SourcePath),
                WccLinuxDiskNative.Descriptor(stage.Lease.ParentHandle), Path.GetFileName(stage.BackupPath!), 1);
            GC.KeepAlive(source);
            return result == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        using var handle = CreateFileWindows(original.SourcePath, GenericRead | DeleteAccess, FileShareRead,
            IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) return Marshal.GetLastWin32Error();
        var observed = CaptureRawFile(handle);
        if (observed.Identity != original.SourceIdentity || observed.NumberOfLinks != 1
            || observed.Bytes.LongLength != original.RawByteLength || HashBytes(observed.Bytes) != original.RawByteSha256)
            throw new IOException("The deletion source no longer matches its reviewed identity and bytes.");
        stage.Lease.VerifyCurrentRoute();
        var disposition = new FileDispositionInformation { DeleteFile = 1 };
        return SetFileInformationByHandleWindows(handle, FileDispositionInfo, ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>()) ? 0 : Marshal.GetLastWin32Error();
    }

    private static bool IsExactDeletedBackup(MixedBatchStage stage)
    {
        var expected = stage.Mutation.Deletion!.Expected;
        var captured = WccTextDiskRevisionCapture.CaptureBinary(expected.ProjectRootPath, stage.BackupPath);
        return captured.IsCaptured && captured.Revision is { } backup
            && backup.RawByteSha256 == expected.RawByteSha256 && backup.RawByteLength == expected.RawByteLength
            && backup.ProjectRootIdentity == expected.ProjectRootIdentity
            && backup.SourceParentIdentity == expected.SourceParentIdentity && backup.RouteIdentitySha256 == expected.RouteIdentitySha256
            && backup.SourceHardLinkCount == 1
            && backup.SourceIdentity == (OperatingSystem.IsLinux() ? expected.SourceIdentity : stage.Identity);
    }

    private static string? CleanupDeletedBackup(MixedBatchStage stage)
    {
        if (OperatingSystem.IsLinux())
            return "The owned file was removed from its active path. Linux retained verified recovery copies at "
                + stage.BackupPath + " and " + stage.StagePath + ".";
        return TryDeleteOwnedFile(stage.BackupPath!, stage.Identity!, stage.Bytes!.LongLength, HashBytes(stage.Bytes), 1);
    }
}