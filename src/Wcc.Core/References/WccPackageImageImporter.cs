using System.Security.Cryptography;

namespace Wcc.Core.References;

public enum WccPackageImageTarget
{
    UnitImage,
    Portrait,
    AttackIcon
}

public enum WccPackageImageImportStatus
{
    Imported,
    ReusedExisting,
    AlreadyOwned,
    InvalidPackageRoot,
    InvalidSource,
    SourceMissing,
    UnsupportedFileType,
    IoFailure,
    InvalidUnitId,
    DestinationConflict
}

public sealed record WccPackageImageImportResult(
    WccPackageImageImportStatus Status,
    string? WmlPath = null,
    string? DestinationPath = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Status is
        WccPackageImageImportStatus.Imported or
        WccPackageImageImportStatus.ReusedExisting or
        WccPackageImageImportStatus.AlreadyOwned;
}

/// <summary>
/// Imports a user-selected image into a writable Wesnoth package. The returned
/// path is relative to the package's images directory, which is the form WML
/// image fields expect. Existing package-owned images are reused, identical
/// collisions are de-duplicated, and different collisions receive a suffix.
/// When a unit ID is supplied, new images use that unit's conventional folder
/// and readable resource name; existing owned image paths remain in place.
/// </summary>
public static class WccPackageImageImporter
{
    private static readonly HashSet<string> SupportedExtensions = new(
        [".png", ".webp", ".jpg", ".jpeg"],
        StringComparer.OrdinalIgnoreCase);

    public static WccPackageImageImportResult Import(
        string packageRoot,
        string sourcePath,
        WccPackageImageTarget target,
        string? unitId = null)
    {
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            return new WccPackageImageImportResult(
                WccPackageImageImportStatus.InvalidPackageRoot,
                ErrorMessage: "A writable package root is required.");
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new WccPackageImageImportResult(
                WccPackageImageImportStatus.InvalidSource,
                ErrorMessage: "An image source path is required.");
        }

        if (unitId is not null && WccUnitResourcePathPolicy.GetUnitIdProblem(unitId) is { } idProblem)
            return new(WccPackageImageImportStatus.InvalidUnitId, ErrorMessage: idProblem);

        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
            var fullSource = Path.GetFullPath(sourcePath);
            if (!Directory.Exists(fullRoot))
            {
                return new WccPackageImageImportResult(
                    WccPackageImageImportStatus.InvalidPackageRoot,
                    ErrorMessage: "The writable package root does not exist.");
            }

            if (!File.Exists(fullSource))
            {
                return new WccPackageImageImportResult(
                    WccPackageImageImportStatus.SourceMissing,
                    ErrorMessage: "The selected image no longer exists.");
            }

            var extension = Path.GetExtension(fullSource);
            if (!SupportedExtensions.Contains(extension))
            {
                return new WccPackageImageImportResult(
                    WccPackageImageImportStatus.UnsupportedFileType,
                    ErrorMessage: "WCC currently imports PNG, WebP, JPG, and JPEG images.");
            }

            var imagesRoot = Path.GetFullPath(Path.Combine(fullRoot, "images"));
            if (TryGetPathInsideRoot(imagesRoot, fullSource, out var existingRelative))
            {
                return new WccPackageImageImportResult(
                    WccPackageImageImportStatus.AlreadyOwned,
                    ToWmlPath(existingRelative),
                    fullSource);
            }

            var targetFolderName = target switch
            {
                WccPackageImageTarget.UnitImage => "units",
                WccPackageImageTarget.Portrait => "portraits",
                WccPackageImageTarget.AttackIcon => "attacks",
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
            };
            var unitStem = unitId is null ? null : WccUnitResourcePathPolicy.GetUnitStem(unitId);
            var targetDirectory = Path.GetFullPath(unitStem is null
                ? Path.Combine(imagesRoot, targetFolderName)
                : Path.Combine(imagesRoot, targetFolderName, unitStem));
            if (!TryGetPathInsideRoot(imagesRoot, targetDirectory, out _))
            {
                return new WccPackageImageImportResult(
                    WccPackageImageImportStatus.InvalidPackageRoot,
                    ErrorMessage: "The image destination escaped the writable package root.");
            }

            if (unitId is not null)
            {
                if (CreateExactUnitDirectory(fullRoot, targetFolderName, unitStem!) is { } directoryProblem)
                    return new(WccPackageImageImportStatus.DestinationConflict, ErrorMessage: directoryProblem);
            }
            else Directory.CreateDirectory(targetDirectory);

            var sourceFileName = Path.GetFileName(fullSource);
            var stem = Path.GetFileNameWithoutExtension(sourceFileName);
            for (var suffix = 1; ; suffix++)
            {
                var candidateName = unitId is not null
                    ? Path.GetFileName(WccUnitResourcePathPolicy.GetRelativePath("images", targetFolderName, unitId, extension, suffix))
                    : suffix == 1
                    ? sourceFileName
                    : $"{stem}_{suffix}{extension}";
                var candidatePath = Path.Combine(targetDirectory, candidateName);

                if (unitId is not null && ExactFileProblem(targetDirectory, candidateName) is { } fileProblem)
                    return new(WccPackageImageImportStatus.DestinationConflict, ErrorMessage: fileProblem);

                if (File.Exists(candidatePath))
                {
                    if (FilesHaveSameContent(fullSource, candidatePath))
                    {
                        return Success(
                            WccPackageImageImportStatus.ReusedExisting,
                            imagesRoot,
                            candidatePath);
                    }

                    continue;
                }

                var temporaryPath = Path.Combine(
                    targetDirectory,
                    $".{candidateName}.{Guid.NewGuid():N}.wcc.tmp");
                try
                {
                    File.Copy(fullSource, temporaryPath, overwrite: false);
                    try
                    {
                        File.Move(temporaryPath, candidatePath, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(candidatePath))
                    {
                        // A concurrent import claimed this name. Compare or try
                        // the next collision-safe suffix on the next iteration.
                        continue;
                    }

                    return Success(
                        WccPackageImageImportStatus.Imported,
                        imagesRoot,
                        candidatePath);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            CryptographicException)
        {
            return new WccPackageImageImportResult(
                WccPackageImageImportStatus.IoFailure,
                ErrorMessage: ex.Message);
        }
    }

    private static string? CreateExactUnitDirectory(string packageRoot, string category, string unitId)
    {
        var current = packageRoot;
        foreach (var name in new[] { "images", category, unitId })
        {
            var requested = Path.Combine(current, name);
            string? CheckExisting()
            {
                var matches = Directory.EnumerateFileSystemEntries(current)
                    .Where(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 0) return null;
                if (matches.Length != 1 || !string.Equals(Path.GetFileName(matches[0]), name, StringComparison.Ordinal) ||
                    !Directory.Exists(matches[0]) || (File.GetAttributes(matches[0]) & FileAttributes.ReparsePoint) != 0)
                    return "Unit image folder '" + requested + "' conflicts with an existing name, case or linked directory.";
                return null;
            }
            if (CheckExisting() is { } problem) return problem;
            Directory.CreateDirectory(requested);
            if (CheckExisting() is { } createdProblem) return createdProblem;
            current = requested;
        }
        return null;
    }

    private static string? ExactFileProblem(string directory, string name)
    {
        var matches = Directory.EnumerateFileSystemEntries(directory)
            .Where(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1 || !string.Equals(Path.GetFileName(matches[0]), name, StringComparison.Ordinal) ||
            !File.Exists(matches[0]) || (File.GetAttributes(matches[0]) & FileAttributes.ReparsePoint) != 0)
            return "Unit image filename '" + Path.Combine(directory, name) + "' conflicts with an existing name, case or linked file.";
        return null;
    }

    private static WccPackageImageImportResult Success(
        WccPackageImageImportStatus status,
        string imagesRoot,
        string destinationPath)
    {
        var relative = Path.GetRelativePath(imagesRoot, destinationPath);
        return new WccPackageImageImportResult(
            status,
            ToWmlPath(relative),
            destinationPath);
    }

    private static bool FilesHaveSameContent(string firstPath, string secondPath)
    {
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        if (firstInfo.Length != secondInfo.Length)
            return false;

        using var first = File.OpenRead(firstPath);
        using var second = File.OpenRead(secondPath);
        var firstHash = SHA256.HashData(first);
        var secondHash = SHA256.HashData(second);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static bool TryGetPathInsideRoot(
        string root,
        string path,
        out string relativePath)
    {
        relativePath = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relativePath) &&
               !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
               !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string ToWmlPath(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
}
