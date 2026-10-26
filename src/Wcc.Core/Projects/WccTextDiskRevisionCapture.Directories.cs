using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wcc.Core.Projects;

public sealed partial class WccTextDiskRevisionCapture
{
    // FILE_CREATE returns our directory handle atomically. An existing child is never adopted.
    internal static WccOwnedDirectoryCreation CreatePinnedDirectory(WccTextDiskFileAbsence expected) =>
        CreatePinnedDirectoryWithVerificationHook(expected, null);

    private static WccOwnedDirectoryCreation CreatePinnedDirectoryWithVerificationHook(
        WccTextDiskFileAbsence expected, Action? afterNativeCreation)
    {
        if (expected.RequiredDirectories.Count != 0
            || Path.GetDirectoryName(expected.SourcePath) != expected.SourceParentPath)
            throw new InvalidDataException("Directory creation needs the exact existing pinned parent.");
        if (OperatingSystem.IsLinux()) return CreatePinnedLinuxDirectory(expected, afterNativeCreation);
        var route = AcquireAbsenceRouteLease(expected);
        SafeFileHandle? created = null;
        var nameBuffer = IntPtr.Zero;
        var unicodeBuffer = IntPtr.Zero;
        var nativeCreated = false;
        try
        {
            var name = Path.GetFileName(expected.SourcePath);
            if (!IsPortableNewFilename(name)) throw new InvalidDataException("The new directory name is not portable.");
            nameBuffer = Marshal.StringToHGlobalUni(name);
            var unicode = new DirectoryUnicodeString
            { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)((name.Length + 1) * 2)), Buffer = nameBuffer };
            unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<DirectoryUnicodeString>());
            Marshal.StructureToPtr(unicode, unicodeBuffer, false);
            var attributes = new DirectoryObjectAttributes
            { Length = Marshal.SizeOf<DirectoryObjectAttributes>(), RootDirectory = route.ParentHandle.DangerousGetHandle(),
                ObjectName = unicodeBuffer, Attributes = 0x40 }; // OBJ_CASE_INSENSITIVE
            // FILE_CREATE cannot adopt an existing leaf, including an existing reparse point.
            // Use NT directory-compatible options, rather than flags intended for Win32 opens.
            var status = NtCreateDirectoryFile(out var nativeHandle, FileListDirectory | NtFileReadAttributes | NtSynchronize,
                ref attributes, out _, IntPtr.Zero, NtFileAttributeNormal, FileShareRead | FileShareWrite,
                NtFileCreate, NtFileDirectoryFile | NtFileSynchronousIoNonalert, IntPtr.Zero, 0);
            if (status < 0)
            {
                var error = checked((int)RtlNtStatusToDosError(status));
                throw new Win32Exception(error, "Windows refused exclusive creation of the reviewed directory"
                    + $" (NTSTATUS=0x{unchecked((uint)status):X8}, NativeErrorCode={error}): " + new Win32Exception(error).Message);
            }
            nativeCreated = true;
            created = new SafeFileHandle(nativeHandle, ownsHandle: true);
            afterNativeCreation?.Invoke();
            var snapshot = ReadHandleSnapshot(created);
            RejectReparsePoint(expected.SourcePath, snapshot);
            if (!snapshot.IsDirectory || snapshot.IsDevice)
                throw new InvalidDataException("The created directory is not an ordinary directory.");
            var result = new WccOwnedDirectoryCreation(expected.SourcePath, snapshot.Identity, created, route);
            created = null;
            route = null!;
            return result;
        }
        catch (Exception exception) when (nativeCreated)
        {
            // This is already a disk mutation. Do not disguise a metadata failure as a no-write refusal.
            throw new IOException("The reviewed directory was created but could not be verified; retain it for recovery: "
                + expected.SourcePath + ". " + exception.Message, exception);
        }
        finally
        {
            created?.Dispose(); route?.Dispose();
            if (unicodeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(unicodeBuffer);
            if (nameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private const uint NtSynchronize = 0x00100000;
    private const uint NtFileReadAttributes = 0x00000080;
    private const uint NtFileAttributeNormal = 0x00000080;
    private const uint NtFileCreate = 2;
    private const uint NtFileDirectoryFile = 0x00000001;
    private const uint NtFileSynchronousIoNonalert = 0x00000020;

    [StructLayout(LayoutKind.Sequential)]
    private struct DirectoryUnicodeString { internal ushort Length; internal ushort MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DirectoryObjectAttributes
    {
        internal int Length; internal IntPtr RootDirectory; internal IntPtr ObjectName; internal uint Attributes;
        internal IntPtr SecurityDescriptor; internal IntPtr SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DirectoryIoStatusBlock { internal IntPtr Status; internal nuint Information; }
    [DllImport("ntdll.dll", EntryPoint = "NtCreateFile", ExactSpelling = true)]
    private static extern int NtCreateDirectoryFile(out IntPtr fileHandle, uint desiredAccess,
        ref DirectoryObjectAttributes objectAttributes, out DirectoryIoStatusBlock ioStatusBlock,
        IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition,
        uint createOptions, IntPtr eaBuffer, uint eaLength);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
}

internal sealed class WccOwnedDirectoryCreation(string path, WccWindowsFileIdentity identity,
    SafeFileHandle handle, WccTextDiskRouteLease route) : IDisposable
{
    internal string Path { get; } = path;
    internal WccWindowsFileIdentity Identity { get; } = identity;
    public void Dispose() { handle.Dispose(); route.Dispose(); }
}
