using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Wcc.Core.Projects.Creation;

/// <summary>
/// Creates a new source project in an exclusively owned sibling stage and publishes
/// it once. Existing projects are never patched or replaced by this workflow.
/// </summary>
public static class WccCampaignCreator
{
    public static async Task<WccCampaignCreationResult> CreateAsync(WccCampaignCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        // Freeze the caller's difficulty collection before the first asynchronous boundary.
        request = request with { Difficulties = request.Difficulties?.ToArray() ?? [],
            Chapters = request.Chapters?.Select(chapter => chapter is null ? null! : chapter with { Difficulties = chapter.Difficulties?.ToArray() ?? [] }).ToArray() ?? [],
            Dependencies = request.Dependencies?.Select(dependency => dependency is null ? null! : new Wcc.Core.Campaigns.WccCampaignDependencySpec(
                dependency.Id, dependency.AddonId, dependency.TargetCampaignDefines, dependency.Steps, dependency.DeclareDependency, dependency.AppliesToAllCampaigns)).ToArray() ?? [] };
        var plan = WccCampaignCreationPlanner.Plan(request);
        var parent = Path.GetDirectoryName(plan.RootPath)!;
        var lockPath = Path.Combine(parent, ".wcc-create-" + request.Id.ToLowerInvariant() + ".lock");
        var stagePath = Path.Combine(parent, "." + request.Id + ".wcc-create-" + Guid.NewGuid().ToString("N"));
        var ownedDirectories = new List<string>();
        var ownedFiles = new List<string>();
        var ownsStage = false;
        var published = false;
        FileStream? reservation = null;
        try
        {
            WccCampaignCreationPlanner.ValidateDirectoryRoute(parent);
            // All WCC creators reserve the same portable, case-insensitive ID.
            reservation = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDestinationAbsent(parent, request.Id);
            CreateDirectoryExclusive(stagePath);
            ownsStage = true;
            ownedDirectories.Add(stagePath);
            foreach (var directory in plan.DirectoryPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(plan.RootPath, directory);
                var destination = InsideStage(stagePath, relative);
                WccCampaignCreationPlanner.ValidateDirectoryRoute(Path.GetDirectoryName(destination)!);
                CreateDirectoryExclusive(destination);
                ownedDirectories.Add(destination);
            }
            foreach (var file in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = InsideStage(stagePath, file.RelativePath);
                WccCampaignCreationPlanner.ValidateDirectoryRoute(Path.GetDirectoryName(destination)!);
                // Track ownership only after CreateNew succeeds; a conflicting
                // external file must never become a cleanup target.
                await WccCreationSourceWriter.WriteNewTextAsync(destination, file.Text, cancellationToken,
                    ownedFiles.Add).ConfigureAwait(false);
            }
            foreach (var asset in plan.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = InsideStage(stagePath, asset.RelativePath);
                WccCampaignCreationPlanner.ValidateDirectoryRoute(Path.GetDirectoryName(destination)!);
                await WccCreationSourceWriter.CopyNewAssetAsync(asset, destination, cancellationToken,
                    ownedFiles.Add).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await new JsonWccProjectMetadataStore(stagePath).SaveNewAsync(plan.Metadata, cancellationToken).ConfigureAwait(false);
            ownedFiles.Add(Path.Combine(stagePath, ".wcc", "project.json"));
            cancellationToken.ThrowIfCancellationRequested();
            WccCampaignCreationPlanner.ValidateDirectoryRoute(stagePath);
            EnsureDestinationAbsent(parent, request.Id);
            PublishWithoutReplacement(stagePath, plan.RootPath);
            published = true;
            // Publication is the commit point. Cancellation afterward cannot undo
            // an already created campaign or turn success into an ambiguous failure.
            return new(plan.RootPath, plan.MainFilePath, plan.Metadata);
        }
        finally
        {
            try
            {
                if (ownsStage && !published) CleanupOwnedStage(parent, stagePath, ownedFiles, ownedDirectories);
            }
            finally
            {
                if (reservation is not null)
                {
                    try
                    {
                        reservation.Dispose();
                        WccCampaignCreationPlanner.ValidateDirectoryRoute(parent);
                        File.Delete(lockPath);
                    }
                    catch (Exception error) when (published && error is IOException or UnauthorizedAccessException)
                    {
                        // The campaign was committed. A reservation-close error
                        // cannot turn that successful publication into a failure.
                    }
                }
            }
        }
    }

    private static void EnsureDestinationAbsent(string parent, string id)
    {
        WccCampaignCreationPlanner.ValidateDirectoryRoute(parent);
        if (Directory.EnumerateFileSystemEntries(parent).Any(entry =>
            string.Equals(Path.GetFileName(entry), id, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The campaign destination already exists; creation never replaces it.");
    }

    private static string InsideStage(string stage, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new IOException("A creation recipe must use relative paths.");
        var path = Path.GetFullPath(Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(stage + Path.DirectorySeparatorChar, comparison))
            throw new IOException("A creation recipe escapes its owned staging directory.");
        return path;
    }

    private static void CleanupOwnedStage(string parent, string stage, IReadOnlyList<string> files, IReadOnlyList<string> directories)
    {
        // Delete only paths this call created, never recursively follow or erase
        // an unexpected directory tree. An externally changed route is retained.
        if (!string.Equals(Path.GetDirectoryName(stage), parent,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("The staging cleanup target left its selected parent.");
        WccCampaignCreationPlanner.ValidateDirectoryRoute(stage);
        foreach (var file in files.Reverse())
        {
            WccCampaignCreationPlanner.ValidateDirectoryRoute(Path.GetDirectoryName(file)!);
            if (File.Exists(file)) File.Delete(file);
        }
        foreach (var directory in directories.Reverse())
        {
            WccCampaignCreationPlanner.ValidateDirectoryRoute(directory);
            Directory.Delete(directory, recursive: false);
        }
    }

    private static void CreateDirectoryExclusive(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateDirectoryWindows(path, IntPtr.Zero)) throw NativeFailure("create staging directory", path);
        }
        else if (OperatingSystem.IsLinux())
        {
            if (CreateDirectoryLinux(path, 0x1C0) != 0) throw NativeFailure("create staging directory", path); // 0700
        }
        else throw new PlatformNotSupportedException("Campaign creation supports Windows and Linux.");
    }

    private static void PublishWithoutReplacement(string stage, string destination)
    {
        if (OperatingSystem.IsWindows()) Directory.Move(stage, destination);
        else if (OperatingSystem.IsLinux())
        {
            // POSIX rename may replace an empty destination directory. Linux's
            // explicit NOREPLACE operation retains the same exclusive contract.
            try
            {
                if (RenameDirectoryLinux(-100, stage, -100, destination, 1) != 0)
                    throw NativeFailure("publish campaign without replacement", destination);
            }
            catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
            { throw new IOException("This Linux runtime cannot publish a campaign without replacement.", error); }
        }
        else throw new PlatformNotSupportedException("Campaign creation supports Windows and Linux.");
    }

    private static IOException NativeFailure(string operation, string path) =>
        new("Could not " + operation + " '" + path + "'.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWindows(string path, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int CreateDirectoryLinux([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameDirectoryLinux(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
}
