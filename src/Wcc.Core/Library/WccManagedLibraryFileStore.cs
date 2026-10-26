using System.Text;

namespace Wcc.Core.Library;

public enum WccManagedLibraryFileKind
{
    UnitType,
    Race,
    MovementType,
    Trait,
    TerrainType,
    Item,
    Macro,
    Schedule,
    Ability,
    WeaponSpecial,
    AbilityRegistry,
    WeaponSpecialRegistry
}

public enum WccManagedLibraryMutationStatus
{
    Success,
    InvalidSource,
    OutsideManagedKindDirectory,
    SourceMissing,
    ExternalChangeDetected,
    IoFailure
}

public sealed record WccManagedLibraryMutationResult(
    WccManagedLibraryMutationStatus Status,
    string? SourcePath = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Status == WccManagedLibraryMutationStatus.Success;
}

/// <summary>
/// Physical mutation boundary for WCC-owned package files. The package index remains the
/// catalog and identity authority; this class only creates, replaces, or removes the cfg
/// source represented by a managed-library entity.
/// </summary>
public static class WccManagedLibraryFileStore
{
    public static WccManagedLibraryMutationResult Create(
        string libraryRoot,
        WccManagedLibraryFileKind kind,
        string rawText,
        string? storageId = null)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || rawText is null)
        {
            return new WccManagedLibraryMutationResult(
                WccManagedLibraryMutationStatus.InvalidSource,
                ErrorMessage: "A managed library root and source text are required.");
        }

        try
        {
            var directory = GetKindDirectory(libraryRoot, kind);
            Directory.CreateDirectory(directory);

            var fileStem = string.IsNullOrWhiteSpace(storageId)
                ? WccInternalId.New(GetStoragePrefix(kind))
                : Path.GetFileNameWithoutExtension(storageId.Trim());
            if (string.IsNullOrWhiteSpace(fileStem) ||
                fileStem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return new WccManagedLibraryMutationResult(
                    WccManagedLibraryMutationStatus.InvalidSource,
                    ErrorMessage: "The managed file identity is invalid.");
            }

            var path = Path.Combine(directory, fileStem + ".cfg");
            if (File.Exists(path))
            {
                return new WccManagedLibraryMutationResult(
                    WccManagedLibraryMutationStatus.InvalidSource,
                    path,
                    "A managed file with this identity already exists.");
            }

            WriteNewAtomically(path, rawText);
            return new WccManagedLibraryMutationResult(
                WccManagedLibraryMutationStatus.Success,
                path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new WccManagedLibraryMutationResult(
                WccManagedLibraryMutationStatus.IoFailure,
                ErrorMessage: ex.Message);
        }
    }

    public static WccManagedLibraryMutationResult Update(
        string libraryRoot,
        WccManagedLibraryFileKind kind,
        string? sourcePath,
        string rawText,
        string? expectedContentHash = null)
    {
        var validation = ValidateExistingPath(libraryRoot, kind, sourcePath);
        if (!validation.IsSuccess)
            return validation;

        var fullPath = validation.SourcePath!;
        try
        {
            if (!MatchesExpectedContent(fullPath, expectedContentHash))
            {
                return new WccManagedLibraryMutationResult(
                    WccManagedLibraryMutationStatus.ExternalChangeDetected,
                    fullPath,
                    "The managed cfg changed on disk after it was indexed or loaded.");
            }

            WriteAtomically(fullPath, rawText);
            return new WccManagedLibraryMutationResult(
                WccManagedLibraryMutationStatus.Success,
                fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new WccManagedLibraryMutationResult(
                WccManagedLibraryMutationStatus.IoFailure,
                fullPath,
                ex.Message);
        }
    }

    public static WccManagedLibraryMutationResult Delete(
        string libraryRoot,
        WccManagedLibraryFileKind kind,
        string? sourcePath,
        string? expectedContentHash = null)
    {
        var validation = ValidateExistingPath(libraryRoot, kind, sourcePath);
        if (!validation.IsSuccess)
            return validation;

        var fullPath = validation.SourcePath!;
        try
        {
            if (!MatchesExpectedContent(fullPath, expectedContentHash))
            {
                return new WccManagedLibraryMutationResult(
                    WccManagedLibraryMutationStatus.ExternalChangeDetected,
                    fullPath,
                    "The managed cfg changed on disk after it was indexed or loaded.");
            }

            File.Delete(fullPath);
            return new WccManagedLibraryMutationResult(
                WccManagedLibraryMutationStatus.Success,
                fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new WccManagedLibraryMutationResult(
                WccManagedLibraryMutationStatus.IoFailure,
                fullPath,
                ex.Message);
        }
    }

    public static bool IsPathForKind(
        string libraryRoot,
        WccManagedLibraryFileKind kind,
        string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(sourcePath))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(sourcePath);
            var directory = GetKindDirectory(libraryRoot, kind);
            return IsPathInsideDirectory(fullPath, directory) &&
                   string.Equals(Path.GetExtension(fullPath), ".cfg", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static string GetKindDirectory(
        string libraryRoot,
        WccManagedLibraryFileKind kind)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            throw new ArgumentException("Managed library root must be set.", nameof(libraryRoot));

        return Path.GetFullPath(Path.Combine(
            libraryRoot,
            GetCatalogFolderName(kind),
            "snapshots"));
    }

    private static WccManagedLibraryMutationResult ValidateExistingPath(
        string libraryRoot,
        WccManagedLibraryFileKind kind,
        string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new WccManagedLibraryMutationResult(
                WccManagedLibraryMutationStatus.InvalidSource,
                ErrorMessage: "The managed cfg source path is missing.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new WccManagedLibraryMutationResult(
                WccManagedLibraryMutationStatus.InvalidSource,
                sourcePath,
                ex.Message);
        }

        if (!IsPathForKind(libraryRoot, kind, fullPath))
        {
            return new WccManagedLibraryMutationResult(
                WccManagedLibraryMutationStatus.OutsideManagedKindDirectory,
                fullPath,
                "The cfg is not inside the expected managed-library content folder.");
        }

        if (!File.Exists(fullPath))
        {
            return new WccManagedLibraryMutationResult(
                WccManagedLibraryMutationStatus.SourceMissing,
                fullPath,
                "The managed cfg no longer exists.");
        }

        return new WccManagedLibraryMutationResult(
            WccManagedLibraryMutationStatus.Success,
            fullPath);
    }

    private static bool MatchesExpectedContent(string path, string? expectedContentHash)
    {
        if (string.IsNullOrWhiteSpace(expectedContentHash))
            return true;

        var currentText = File.ReadAllText(path);
        return string.Equals(
            WccContentHash.ComputeText(currentText),
            expectedContentHash,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteNewAtomically(string path, string rawText)
    {
        var temporaryPath = path + ".wcc-new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, rawText, new UTF8Encoding(false));
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static void WriteAtomically(string path, string rawText)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("The managed cfg has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporaryPath, rawText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullDirectory, comparison);
    }

    private static string GetCatalogFolderName(WccManagedLibraryFileKind kind)
        => kind switch
        {
            WccManagedLibraryFileKind.UnitType => "unit-catalog",
            WccManagedLibraryFileKind.Race => "race-catalog",
            WccManagedLibraryFileKind.MovementType => "movement-type-catalog",
            WccManagedLibraryFileKind.Trait => "trait-catalog",
            WccManagedLibraryFileKind.TerrainType => "terrain-type-catalog",
            WccManagedLibraryFileKind.Item => "item-catalog",
            WccManagedLibraryFileKind.Macro => "macro-catalog",
            WccManagedLibraryFileKind.Schedule => "schedule-preset-catalog",
            WccManagedLibraryFileKind.Ability => "ability-preset-catalog",
            WccManagedLibraryFileKind.WeaponSpecial => "weapon-special-preset-catalog",
            WccManagedLibraryFileKind.AbilityRegistry => "ability-registry-catalog",
            WccManagedLibraryFileKind.WeaponSpecialRegistry => "weapon-special-registry-catalog",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static string GetStoragePrefix(WccManagedLibraryFileKind kind)
        => kind switch
        {
            WccManagedLibraryFileKind.UnitType => "unitfile",
            WccManagedLibraryFileKind.Race => "race",
            WccManagedLibraryFileKind.MovementType => "movetype",
            WccManagedLibraryFileKind.Trait => "trait",
            WccManagedLibraryFileKind.TerrainType => "terraintype",
            WccManagedLibraryFileKind.Item => "item",
            WccManagedLibraryFileKind.Macro => "macro",
            WccManagedLibraryFileKind.Schedule => "schedule",
            WccManagedLibraryFileKind.Ability => "ability",
            WccManagedLibraryFileKind.WeaponSpecial => "weaponspecial",
            WccManagedLibraryFileKind.AbilityRegistry => "abilityregistry",
            WccManagedLibraryFileKind.WeaponSpecialRegistry => "weaponspecialregistry",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}
