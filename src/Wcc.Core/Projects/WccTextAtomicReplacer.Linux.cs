using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wcc.Core.Projects;

public sealed partial class WccTextAtomicReplacer
{
    // Directory descriptors pin objects, not Linux namespace entries. Every
    // publication is relative to those descriptors and verifies the live route
    // again; a detected move retains evidence instead of attempting rollback.
    private static void WriteExclusiveLinux(WccTextDiskRouteLease lease, string stagePath,
        byte[] bytes, out WccWindowsFileIdentity? identity)
    {
        identity = null;
        lease.VerifyCurrentRoute();
        using var handle = WccLinuxDiskNative.OpenAt(lease.ParentHandle, Path.GetFileName(stagePath),
            WccLinuxDiskNative.ReadWrite | WccLinuxDiskNative.Create | WccLinuxDiskNative.Exclusive
            | WccLinuxDiskNative.NoFollow | WccLinuxDiskNative.CloseOnExec | WccLinuxDiskNative.NonBlocking, 0x180);
        var snapshot = WccLinuxDiskNative.ReadSnapshot(handle);
        if (!snapshot.IsRegular || snapshot.LinkCount != 1 || snapshot.Length != 0)
            throw new IOException("The exclusive Linux staging descriptor is not a new regular file.");
        identity = snapshot.Identity;
        using var fileLock = WccLinuxDiskNative.AcquireExclusiveLock(handle);
        RejectExtendedAccessAclLinux(handle);
        using (var stream = new FileStream(handle, FileAccess.ReadWrite))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        lease.VerifyCurrentRoute();
    }

    private static int ReplaceLinux(WccTextDiskRevision expected, string stagePath,
        WccWindowsFileIdentity stageIdentity, WccTextDiskRouteLease lease, byte[] reviewedBytes)
    {
        lease.VerifyCurrentRoute();
        using var source = OpenPublicationFileLinux(lease, expected.SourcePath);
        using var stage = OpenPublicationFileLinux(lease, stagePath);
        using var sourceLock = WccLinuxDiskNative.AcquireExclusiveLock(source);
        using var stageLock = WccLinuxDiskNative.AcquireExclusiveLock(stage);
        RejectExtendedAccessAclLinux(source);
        RejectExtendedAccessAclLinux(stage);
        var original = CapturePublicationFileLinux(source);
        var candidate = CapturePublicationFileLinux(stage);
        if (original.Metadata.Identity != expected.SourceIdentity
            || original.Metadata.LinkCount != expected.SourceHardLinkCount
            || original.Metadata.Length != expected.RawByteLength || original.Hash != expected.RawByteSha256
            || candidate.Metadata.Identity != stageIdentity || candidate.Metadata.LinkCount != 1
            || candidate.Metadata.Length != reviewedBytes.LongLength || candidate.Hash != HashBytes(reviewedBytes))
            throw new WccTextDiskRouteChangedException("A Linux publication source or staging inode changed before exchange.");

        // Replacement retains source ownership/permissions. Do not silently
        // broaden access with a newly-created inode or ignore metadata errors.
        if (candidate.Metadata.UserId != original.Metadata.UserId || candidate.Metadata.GroupId != original.Metadata.GroupId)
            CheckLinuxResult(ChangeOwnerLinux(WccLinuxDiskNative.Descriptor(stage), original.Metadata.UserId,
                original.Metadata.GroupId), "preserve source ownership");
        CheckLinuxResult(ChangeModeLinux(WccLinuxDiskNative.Descriptor(stage), (uint)(original.Metadata.Mode & 0xfff)),
            "preserve source permissions");
        CheckLinuxResult(SyncLinux(WccLinuxDiskNative.Descriptor(stage)), "flush replacement metadata");
        lease.VerifyCurrentRoute();
        var result = RenameAtLinux(WccLinuxDiskNative.Descriptor(lease.ParentHandle), Path.GetFileName(expected.SourcePath),
            WccLinuxDiskNative.Descriptor(lease.ParentHandle), Path.GetFileName(stagePath), 2); // RENAME_EXCHANGE
        var error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        GC.KeepAlive(lease); GC.KeepAlive(source); GC.KeepAlive(stage);
        return error;
    }

    private static int CreateLinux(string stagePath, string destinationPath, WccWindowsFileIdentity stageIdentity,
        WccTextDiskRouteLease stagingLease, WccTextDiskRouteLease destinationLease, byte[] reviewedBytes)
    {
        stagingLease.VerifyCurrentRoute(); destinationLease.VerifyCurrentRoute();
        using var stage = OpenPublicationFileLinux(stagingLease, stagePath);
        using var stageLock = WccLinuxDiskNative.AcquireExclusiveLock(stage);
        RejectExtendedAccessAclLinux(stage);
        var candidate = CapturePublicationFileLinux(stage);
        if (candidate.Metadata.Identity != stageIdentity || candidate.Metadata.LinkCount != 1
            || candidate.Metadata.Length != reviewedBytes.LongLength || candidate.Hash != HashBytes(reviewedBytes))
            throw new WccTextDiskRouteChangedException("The Linux creation stage no longer has its verified inode.");
        var result = RenameAtLinux(WccLinuxDiskNative.Descriptor(stagingLease.ParentHandle), Path.GetFileName(stagePath),
            WccLinuxDiskNative.Descriptor(destinationLease.ParentHandle), Path.GetFileName(destinationPath), 1); // RENAME_NOREPLACE
        var error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        GC.KeepAlive(stagingLease); GC.KeepAlive(destinationLease); GC.KeepAlive(stage);
        return error;
    }

    private static SafeFileHandle OpenPublicationFileLinux(WccTextDiskRouteLease lease, string path) =>
        WccLinuxDiskNative.OpenAt(lease.ParentHandle, Path.GetFileName(path), WccLinuxDiskNative.ReadOnly
            | WccLinuxDiskNative.NoFollow | WccLinuxDiskNative.CloseOnExec | WccLinuxDiskNative.NonBlocking);

    private static (LinuxFileSnapshot Metadata, string Hash) CapturePublicationFileLinux(SafeFileHandle handle)
    {
        var before = WccLinuxDiskNative.ReadSnapshot(handle);
        if (!before.IsRegular || before.Length > Array.MaxLength)
            throw new IOException("The Linux publication descriptor is not a bounded regular file.");
        var bytes = new byte[(int)before.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (read == 0) throw new IOException("A Linux publication inode changed while reading.");
            offset += read;
        }
        Span<byte> probe = stackalloc byte[1];
        if (RandomAccess.Read(handle, probe, bytes.LongLength) != 0 || before != WccLinuxDiskNative.ReadSnapshot(handle))
            throw new IOException("A Linux publication inode changed while verifying its bytes.");
        return (before, HashBytes(bytes));
    }

    private static void FlushAndVerifyLinuxRoute(WccTextDiskRouteLease lease)
    {
        CheckLinuxResult(SyncLinux(WccLinuxDiskNative.Descriptor(lease.ParentHandle)), "flush publication directory");
        lease.VerifyCurrentRoute();
    }

    private static bool HasCaseEquivalentOtherEntryLinux(MixedBatchStage stage)
    {
        var lease = stage.PublicationLease ?? stage.Lease!;
        lease.VerifyCurrentRoute();
        var name = Path.GetFileName(stage.Mutation.SourcePath);
        var collision = WccLinuxDiskNative.DirectoryNames(lease.ParentHandle).Any(entry =>
            string.Equals(entry, name, StringComparison.OrdinalIgnoreCase) && entry != name);
        lease.VerifyCurrentRoute();
        return collision;
    }

    // A new inode can inherit a named-user/group ACL from its parent. Copying
    // mode bits alone would then grant access absent from the displaced source.
    // Until ACL copying has its own checked contract, refuse either descriptor's
    // extended access ACL rather than erase it or silently broaden permissions.
    private static void RejectExtendedAccessAclLinux(SafeFileHandle handle)
    {
        var length = GetExtendedAttributeLinux(WccLinuxDiskNative.Descriptor(handle), "system.posix_acl_access", IntPtr.Zero, 0);
        var error = length < 0 ? Marshal.GetLastPInvokeError() : 0;
        GC.KeepAlive(handle);
        if (length >= 0) throw new IOException("Linux checked saving cannot yet preserve an extended POSIX access ACL; the source and staging evidence were retained.");
        if (error is not 61 and not 95) // ENODATA, ENOTSUP/EOPNOTSUPP.
            throw new Win32Exception(error, "Linux could not inspect the publication descriptor's POSIX access ACL.");
    }

    private static void CheckLinuxResult(int result, string operation)
    {
        if (result != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Linux could not {operation}.");
    }

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAtLinux(int sourceParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceName,
        int destinationParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string destinationName, uint flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int SyncLinux(int descriptor);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int ChangeModeLinux(int descriptor, uint mode);
    [DllImport("libc", EntryPoint = "fchown", SetLastError = true)]
    private static extern int ChangeOwnerLinux(int descriptor, uint user, uint group);
    [DllImport("libc", EntryPoint = "fgetxattr", SetLastError = true)]
    private static extern nint GetExtendedAttributeLinux(int descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value, nuint size);
}
