using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace Wcc.Core.Projects;

public sealed partial class WccTextDiskRevisionCapture
{
    private static CapturePassResult CaptureLinuxPass(string projectRoot, string sourceParent, string sourcePath,
        long maximumExpectedBytes)
    {
        var chain = BuildDirectoryChain(projectRoot, sourceParent);
        var handles = new List<SafeFileHandle>();
        try
        {
            var identities = OpenLinuxDirectoryChain(chain, handles);
            var source = OpenLinuxComponent(handles[^1], Path.GetFileName(sourcePath), readContents: true, projectRoot: false);
            handles.Add(source);
            var before = ReadHandleSnapshot(source);
            if (before.IsDirectory || before.IsDevice) throw new InvalidSourceException("The source path is not a regular Linux file.");
            using var sourceLock = WccLinuxDiskNative.AcquireSharedLock(source);
            var bytes = ReadAllBytes(source, before.EndOfFile, maximumExpectedBytes);
            var after = ReadHandleSnapshot(source);
            if (!before.IsSameFileState(after) || after.EndOfFile != bytes.LongLength)
                throw new DiskChangedDuringCaptureException("The Linux inode changed while its bytes were being read.");
            // Holding an fd does not lock a Linux pathname. Rewalk from / to prove
            // the opened route still has the same names and objects after reading.
            try
            {
                VerifyLinuxDirectoryRoute(projectRoot, sourceParent, identities[chain.ProjectRootIndex].Identity,
                    identities[^1].Identity, HashRouteIdentities(identities));
            }
            catch (WccTextDiskRouteChangedException error)
            { throw new DiskChangedDuringCaptureException("The Linux source route changed during capture.", error); }
            return new(identities, identities[chain.ProjectRootIndex].Identity, after, bytes);
        }
        finally { DisposeHandles(handles); }
    }

    private static WccTextDiskFileAbsence CaptureLinuxAbsencePass(string root, string parent, string destination)
    {
        var chain = BuildDirectoryChain(root, parent);
        var handles = new List<SafeFileHandle>();
        try
        {
            var identities = OpenLinuxDirectoryChain(chain, handles);
            var name = Path.GetFileName(destination);
            if (WccLinuxDiskNative.EntryExists(handles[^1], name) || WccLinuxDiskNative.DirectoryNames(handles[^1])
                    .Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase)))
                throw new DestinationExistsException("The new filename already exists, including a case-equivalent file or directory.");
            var hash = HashRouteIdentities(identities);
            VerifyLinuxDirectoryRoute(root, parent, identities[chain.ProjectRootIndex].Identity, identities[^1].Identity, hash);
            return new(root, parent, destination, hash, identities[chain.ProjectRootIndex].Identity, identities[^1].Identity);
        }
        finally { DisposeHandles(handles); }
    }

    private static WccTextDiskFileAbsence CaptureLinuxCreationAbsencePass(string root, string parent, string destination)
    {
        var relative = Path.GetRelativePath(root, destination).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (relative.Any(component => !IsPortableNewFilename(component)))
            throw new InvalidSourceException("Every created path component must be a portable ordinary filename.");
        var handles = new List<SafeFileHandle>();
        try
        {
            var rootChain = BuildDirectoryChain(root, root);
            var identities = OpenLinuxDirectoryChain(rootChain, handles).ToList();
            var anchor = root;
            var missing = new List<string>();
            foreach (var component in relative.Take(relative.Length - 1))
            {
                var next = Path.Combine(anchor, component);
                if (missing.Count != 0) { missing.Add(next); anchor = next; continue; }
                var matches = WccLinuxDiskNative.DirectoryNames(handles[^1])
                    .Where(name => string.Equals(name, component, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 0)
                { missing.Add(next); anchor = next; continue; }
                if (matches.Length != 1 || matches[0] != component)
                    throw new InvalidSourceException("The existing creation route has ambiguous or mismatched directory casing.");
                var handle = OpenLinuxComponent(handles[^1], component, readContents: false, projectRoot: false);
                handles.Add(handle); identities.Add(new(next, ReadHandleSnapshot(handle).Identity)); anchor = next;
            }
            if (missing.Count > 16)
                throw new SourceMissingException("A bounded existing project directory is required for creation staging.");
            var existingParent = missing.Count == 0 ? parent : Path.GetDirectoryName(missing[0])!;
            var captured = CaptureLinuxAbsencePass(root, existingParent, missing.Count == 0 ? destination : missing[0]);
            if (captured.ProjectRootIdentity != identities[rootChain.ProjectRootIndex].Identity ||
                captured.SourceParentIdentity != identities[^1].Identity || captured.RouteIdentitySha256 != HashRouteIdentities(identities))
                throw new WccTextDiskRouteChangedException("The Linux creation route changed while its missing descendants were captured.");
            return new(root, existingParent, destination, captured.RouteIdentitySha256,
                captured.ProjectRootIdentity, captured.SourceParentIdentity, missing);
        }
        finally { DisposeHandles(handles); }
    }

    private static WccTextDiskRouteLease AcquireLinuxDirectoryRouteLease(string root, string parent,
        WccWindowsFileIdentity rootIdentity, WccWindowsFileIdentity parentIdentity, string routeHash)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            var chain = BuildDirectoryChain(root, parent);
            var identities = OpenLinuxDirectoryChain(chain, handles);
            CheckLinuxDirectoryIdentities(chain, identities, rootIdentity, parentIdentity, routeHash);
            var parentHandle = handles[^1];
            return new(handles, parentHandle, () =>
            {
                try
                {
                    if (ReadHandleSnapshot(parentHandle).Identity != parentIdentity)
                        throw new WccTextDiskRouteChangedException("The retained Linux parent descriptor changed.");
                    VerifyLinuxDirectoryRoute(root, parent, rootIdentity, parentIdentity, routeHash);
                }
                catch (Exception error) when (error is not WccTextDiskRouteChangedException)
                { throw new WccTextDiskRouteChangedException("The retained Linux route could not be revalidated.", error); }
            });
        }
        catch (Exception error)
        {
            DisposeHandles(handles);
            throw new WccTextDiskRouteChangedException("The Linux project route no longer matches the captured directory identities.", error);
        }
    }

    private static void VerifyLinuxDirectoryRoute(string root, string parent, WccWindowsFileIdentity rootIdentity,
        WccWindowsFileIdentity parentIdentity, string routeHash)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            var chain = BuildDirectoryChain(root, parent);
            CheckLinuxDirectoryIdentities(chain, OpenLinuxDirectoryChain(chain, handles), rootIdentity, parentIdentity, routeHash);
        }
        catch (Exception error) when (error is not WccTextDiskRouteChangedException)
        { throw new WccTextDiskRouteChangedException("The Linux namespace route changed or became unavailable.", error); }
        finally { DisposeHandles(handles); }
    }

    private static void CheckLinuxDirectoryIdentities(DirectoryChain chain, IReadOnlyList<PathIdentity> identities,
        WccWindowsFileIdentity rootIdentity, WccWindowsFileIdentity parentIdentity, string routeHash)
    {
        if (identities[chain.ProjectRootIndex].Identity != rootIdentity || identities[^1].Identity != parentIdentity ||
            !string.Equals(HashRouteIdentities(identities), routeHash, StringComparison.Ordinal))
            throw new WccTextDiskRouteChangedException("The Linux directory route no longer has its reviewed inode identities.");
    }

    private static IReadOnlyList<PathIdentity> OpenLinuxDirectoryChain(DirectoryChain chain, List<SafeFileHandle> handles)
    {
        var identities = new List<PathIdentity>();
        foreach (var path in chain.Paths)
        {
            var handle = OpenLinuxComponent(handles.Count == 0 ? null : handles[^1],
                handles.Count == 0 ? "/" : Path.GetFileName(path), readContents: false,
                projectRoot: identities.Count <= chain.ProjectRootIndex);
            handles.Add(handle);
            var value = ReadHandleSnapshot(handle);
            if (!value.IsDirectory || value.IsDevice)
                throw new InvalidSourceException("The Linux route must consist only of ordinary directories.");
            identities.Add(new(path, value.Identity));
        }
        return identities;
    }

    private static SafeFileHandle OpenLinuxPath(string path, bool readContents, bool projectRoot)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            handles.Add(OpenLinuxComponent(null, "/", false, projectRoot));
            var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < components.Length; index++)
                handles.Add(OpenLinuxComponent(handles[^1], components[index], readContents && index == components.Length - 1, projectRoot));
            var result = handles[^1]; handles.RemoveAt(handles.Count - 1); return result;
        }
        finally { DisposeHandles(handles); }
    }

    private static SafeFileHandle OpenLinuxComponent(SafeFileHandle? parent, string name, bool readContents, bool projectRoot)
    {
        try
        {
            return WccLinuxDiskNative.OpenAt(parent, name, WccLinuxDiskNative.ReadOnly | WccLinuxDiskNative.NoFollow |
                WccLinuxDiskNative.CloseOnExec | (readContents ? WccLinuxDiskNative.NonBlocking : WccLinuxDiskNative.Directory));
        }
        catch (Win32Exception error)
        {
            if ((error.NativeErrorCode is 40 or 20) && WccLinuxDiskNative.IsSymbolicLink(parent, name))
                throw new ReparsePointRejectedException($"Linux symbolic-link component '{name}' is not a checked source route.");
            if (error.NativeErrorCode == 2)
            {
                if (projectRoot) throw new ProjectRootMissingException("The Linux project root directory does not exist.");
                throw new SourceMissingException($"Linux source component '{name}' does not exist.");
            }
            if (error.NativeErrorCode == 20)
            {
                if (projectRoot) throw new InvalidProjectRootException("The Linux project root route contains a non-directory.");
                throw new InvalidSourceException("The Linux source route contains a non-directory.");
            }
            throw;
        }
    }

    private static WccOwnedDirectoryCreation CreatePinnedLinuxDirectory(WccTextDiskFileAbsence expected, Action? afterNativeCreation)
    {
        var route = AcquireAbsenceRouteLease(expected);
        SafeFileHandle? created = null;
        var nativeCreated = false;
        try
        {
            var name = Path.GetFileName(expected.SourcePath);
            if (!IsPortableNewFilename(name) || WccLinuxDiskNative.EntryExists(route.ParentHandle, name) ||
                WccLinuxDiskNative.DirectoryNames(route.ParentHandle).Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The reviewed Linux directory name is no longer exclusively absent.");
            route.VerifyCurrentRoute();
            WccLinuxDiskNative.CreateDirectoryAt(route.ParentHandle, name, 0x1c0); // 0700, owned resource directory
            nativeCreated = true;
            created = OpenLinuxComponent(route.ParentHandle, name, readContents: false, projectRoot: false);
            var snapshot = ReadHandleSnapshot(created);
            if (!snapshot.IsDirectory || snapshot.IsDevice)
                throw new InvalidDataException("The observed created Linux entry is not an ordinary directory.");
            // POSIX mkdirat cannot return a directory fd atomically. Observe it
            // immediately, then verify its inode and parent route after callbacks.
            // Detected namespace interference retains every entry for recovery;
            // no check-then-delete cleanup claims ownership of a mutable name.
            afterNativeCreation?.Invoke();
            route.VerifyCurrentRoute();
            using (var current = OpenLinuxComponent(route.ParentHandle, name, readContents: false, projectRoot: false))
                if (ReadHandleSnapshot(current).Identity != snapshot.Identity)
                    throw new WccTextDiskRouteChangedException("The created Linux directory entry was replaced.");
            var result = new WccOwnedDirectoryCreation(expected.SourcePath, snapshot.Identity, created, route);
            created = null; route = null!; return result;
        }
        catch (Exception error) when (nativeCreated)
        { throw new IOException("The Linux directory was created but could not be verified; retain it for recovery: " + expected.SourcePath, error); }
        finally { created?.Dispose(); route?.Dispose(); }
    }
}
