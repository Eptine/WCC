using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wcc.Core.Projects;

/// <summary>Linux descriptor operations shared by checked capture and persistence.</summary>
internal static class WccLinuxDiskNative
{
    internal const int ReadOnly = 0, ReadWrite = 2, Create = 0x40, Exclusive = 0x80;
    internal const int NonBlocking = 0x800, Directory = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000;
    private const int AtCurrentWorkingDirectory = -100, AtSymlinkNoFollow = 0x100, AtEmptyPath = 0x1000;
    private const uint BasicStats = 0x7ff, MountId = 0x1000, RequiredStats = 0x3df;

    internal static SafeFileHandle OpenAt(SafeFileHandle? parent, string name, int flags, uint mode = 0)
    {
        var descriptor = OpenAtNative(parent is null ? AtCurrentWorkingDirectory : Descriptor(parent), name, flags, mode);
        GC.KeepAlive(parent);
        if (descriptor < 0) throw Error("openat", name);
        return new(new IntPtr(descriptor), ownsHandle: true);
    }

    internal static LinuxFileSnapshot ReadSnapshot(SafeFileHandle handle)
    {
        var result = Statx(Descriptor(handle), "", AtEmptyPath | AtSymlinkNoFollow, BasicStats | MountId, out var value);
        GC.KeepAlive(handle);
        if (result != 0) throw Error("statx", "open descriptor");
        return Snapshot(value);
    }

    internal static IDisposable AcquireSharedLock(SafeFileHandle handle) => new FileLockLease(handle, 1);

    internal static IDisposable AcquireExclusiveLock(SafeFileHandle handle) => new FileLockLease(handle, 2);

    private sealed class FileLockLease : IDisposable
    {
        private SafeFileHandle? _handle;

        public FileLockLease(SafeFileHandle handle, int operation)
        {
            var retained = false;
            try
            {
                // Keep the descriptor valid even if a borrowing FileStream disposes
                // its SafeFileHandle owner before this native lock scope ends.
                handle.DangerousAddRef(ref retained);
                int result;
                do { result = Flock(Descriptor(handle), operation | 4); } // LOCK_NB
                while (result != 0 && Marshal.GetLastPInvokeError() == 4); // EINTR
                if (result != 0) throw Error("flock", "checked file");
                _handle = handle;
            }
            catch
            {
                if (retained) handle.DangerousRelease();
                throw;
            }
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, null);
            if (handle is null) return;
            try
            {
                // flock belongs to the open-file description. Closing only our fd
                // can leave a lock alive on a transient fork/dup copy. Explicitly
                // release before closing, just as the .NET Unix FileStream does.
                // Unlock failure is best-effort; it cannot undo a completed rename.
                int result;
                do { result = Flock(Descriptor(handle), 8); } // LOCK_UN
                while (result != 0 && Marshal.GetLastPInvokeError() == 4);
            }
            finally { handle.DangerousRelease(); }
        }
    }

    internal static bool EntryExists(SafeFileHandle parent, string name)
    {
        var result = Statx(Descriptor(parent), name, AtSymlinkNoFollow, BasicStats, out _);
        GC.KeepAlive(parent);
        if (result == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == 2) return false;
        throw new Win32Exception(error, "Could not inspect a filename relative to its pinned directory.");
    }

    internal static void CreateDirectoryAt(SafeFileHandle parent, string name, uint mode)
    {
        var result = MakeDirectoryAt(Descriptor(parent), name, mode);
        GC.KeepAlive(parent);
        if (result != 0) throw Error("mkdirat", name);
    }

    internal static bool IsSymbolicLink(SafeFileHandle? parent, string name)
    {
        var result = Statx(parent is null ? AtCurrentWorkingDirectory : Descriptor(parent), name,
            AtSymlinkNoFollow, BasicStats, out var value);
        GC.KeepAlive(parent);
        return result == 0 && (value.Mode & 0xf000) == 0xa000;
    }

    internal static IReadOnlyList<string> DirectoryNames(SafeFileHandle parent)
    {
        // A descriptor-relative enumeration cannot be redirected by replacing the
        // pathname. fdopendir owns only our duplicate, never the route's handle.
        var duplicate = Fcntl(Descriptor(parent), 1030, 0); // F_DUPFD_CLOEXEC
        GC.KeepAlive(parent);
        if (duplicate < 0) throw Error("fcntl", "directory descriptor");
        var directory = FdOpenDirectory(duplicate);
        if (directory == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError(); Close(duplicate);
            throw new Win32Exception(error, "Could not enumerate the pinned directory.");
        }
        try
        {
            var result = new List<string>();
            RewindDirectory(directory);
            while (true)
            {
                SetErrno(0);
                var entry = ReadDirectory(directory);
                if (entry == IntPtr.Zero)
                {
                    var error = Marshal.ReadInt32(ErrnoLocation());
                    if (error != 0) throw new Win32Exception(error, "Could not enumerate the pinned directory.");
                    return result;
                }
                // Linux dirent64 has fixed-width inode/offset fields on supported
                // x64/arm64 runtimes; the filename begins at byte 19.
                var length = unchecked((ushort)Marshal.ReadInt16(entry, 16));
                if (length < 20) throw new InvalidDataException("Linux returned an invalid directory entry.");
                var bytes = new byte[length - 19]; Marshal.Copy(IntPtr.Add(entry, 19), bytes, 0, bytes.Length);
                var end = Array.IndexOf(bytes, (byte)0);
                if (end < 0) throw new InvalidDataException("Linux returned an unbounded directory filename.");
                string name;
                try { name = new UTF8Encoding(false, true).GetString(bytes, 0, end); }
                catch (DecoderFallbackException error)
                { throw new InvalidDataException("The pinned directory contains a filename that is not valid UTF-8.", error); }
                if (name is not "." and not "..") result.Add(name);
            }
        }
        finally { CloseDirectory(directory); }
    }

    internal static int Descriptor(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());

    private static LinuxFileSnapshot Snapshot(StatxBuffer value)
    {
        if ((value.Mask & RequiredStats) != RequiredStats || value.Inode == 0 || value.LinkCount == 0 || value.Size > long.MaxValue)
            throw new InvalidDataException("Linux did not return a complete usable inode, link count, mode and byte revision.");
        var identity = new WccWindowsFileIdentity(((ulong)value.DeviceMajor << 32) | value.DeviceMinor,
            value.Inode, (value.Mask & MountId) != 0 ? value.MountId : 0);
        return new(identity, value.Mode, value.UserId, value.GroupId, value.LinkCount, (long)value.Size,
            value.Modified.Seconds, value.Modified.Nanoseconds, value.Changed.Seconds, value.Changed.Nanoseconds);
    }

    private static Win32Exception Error(string operation, string name)
    {
        var error = Marshal.GetLastPInvokeError();
        return new(error, $"Linux {operation} refused '{name}' (errno={error}).");
    }
    private static void SetErrno(int value) => Marshal.WriteInt32(ErrnoLocation(), value);

    // statx's fixed ABI avoids architecture-dependent struct stat layouts.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint LinkCount;
        [FieldOffset(20)] internal uint UserId;
        [FieldOffset(24)] internal uint GroupId;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(96)] internal StatxTimestamp Changed;
        [FieldOffset(112)] internal StatxTimestamp Modified;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
        [FieldOffset(144)] internal ulong MountId;
    }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct StatxTimestamp
    {
        [FieldOffset(0)] internal long Seconds;
        [FieldOffset(8)] internal uint Nanoseconds;
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtNative(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, uint mode);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, uint mask, out StatxBuffer value);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int descriptor, int command, int argument);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int descriptor, int operation);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MakeDirectoryAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);
    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr FdOpenDirectory(int descriptor);
    [DllImport("libc", EntryPoint = "readdir64")]
    private static extern IntPtr ReadDirectory(IntPtr directory);
    [DllImport("libc", EntryPoint = "closedir")]
    private static extern int CloseDirectory(IntPtr directory);
    [DllImport("libc", EntryPoint = "rewinddir")]
    private static extern void RewindDirectory(IntPtr directory);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
    [DllImport("libc", EntryPoint = "__errno_location")]
    private static extern IntPtr ErrnoLocation();
}

internal sealed record LinuxFileSnapshot(WccWindowsFileIdentity Identity, ushort Mode, uint UserId, uint GroupId,
    uint LinkCount, long Length, long ModifiedSeconds, uint ModifiedNanoseconds, long ChangedSeconds, uint ChangedNanoseconds)
{
    internal bool IsDirectory => (Mode & 0xf000) == 0x4000;
    internal bool IsRegular => (Mode & 0xf000) == 0x8000;
    internal bool IsSymbolicLink => (Mode & 0xf000) == 0xa000;
}
