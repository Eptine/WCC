using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wcc.Core.Settings;

public static class WccSettingsStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static WccSettings Load(string settingsFilePath)
    {
        ValidatePath(settingsFilePath);

        if (!File.Exists(settingsFilePath))
            return new WccSettings();

        var json = File.ReadAllText(settingsFilePath);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("WCC settings must contain a JSON object.");

        var migrated = !TryReadFormatVersion(document.RootElement, out var formatVersion);
        WccSettings settings;
        if (migrated || formatVersion < WccSettingsFormat.CurrentVersion)
        {
            if (!migrated && formatVersion <= 0)
                throw new InvalidDataException($"Invalid WCC settings format: {formatVersion}.");

            var legacy = JsonSerializer.Deserialize<LegacyWccSettings>(json, JsonOptions) ?? new();
            settings = MigrateLegacy(legacy);
            migrated = true;
        }
        else
        {
            if (formatVersion > WccSettingsFormat.CurrentVersion)
            {
                throw new NotSupportedException(
                    $"This WCC supports settings format {WccSettingsFormat.CurrentVersion}, " +
                    $"but format {formatVersion} was found.");
            }

            settings = JsonSerializer.Deserialize<WccSettings>(json, JsonOptions) ??
                       new WccSettings();
        }

        var normalized = Normalize(settings);
        if (migrated || normalized)
            Save(settingsFilePath, settings);

        return settings;
    }

    public static void Save(string settingsFilePath, WccSettings settings)
    {
        ValidatePath(settingsFilePath);
        if (settings is null)
            throw new ArgumentNullException(nameof(settings));
        if (settings.FormatVersion > WccSettingsFormat.CurrentVersion)
        {
            throw new NotSupportedException(
                $"This WCC supports settings format {WccSettingsFormat.CurrentVersion}, " +
                $"but format {settings.FormatVersion} was supplied.");
        }

        settings.FormatVersion = WccSettingsFormat.CurrentVersion;
        Normalize(settings);

        var fullPath = Path.GetFullPath(settingsFilePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Could not determine settings directory: {fullPath}");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static WccSettings MigrateLegacy(LegacyWccSettings legacy)
    {
        var settings = new WccSettings
        {
            ImportCoreScenarios = legacy.ImportCoreScenarios
        };
        if (string.IsNullOrWhiteSpace(legacy.WesnothInstallPath) &&
            string.IsNullOrWhiteSpace(legacy.AddonsPath) &&
            legacy.DetectedWesnothInstallation is null)
        {
            return settings;
        }

        var target = legacy.DetectedWesnothInstallation?.CompatibilityTarget;
        var exactVersion = legacy.DetectedWesnothInstallation?.ExactVersion;
        var profile = new WccWesnothInstallationProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = !string.IsNullOrWhiteSpace(target)
                ? $"Wesnoth {target}"
                : !string.IsNullOrWhiteSpace(exactVersion)
                    ? $"Wesnoth {exactVersion}"
                    : "Wesnoth installation",
            InstallPath = legacy.WesnothInstallPath ??
                          legacy.DetectedWesnothInstallation?.InstallRoot,
            AddonsPath = legacy.AddonsPath,
            CompatibilityTarget = target,
            LastDetectedInstallation = legacy.DetectedWesnothInstallation
        };
        settings.WesnothInstallationProfiles.Add(profile);
        settings.ActiveWesnothInstallationProfileId = profile.Id;
        return settings;
    }

    private static bool Normalize(WccSettings settings)
    {
        var changed = false;
        if (settings.FormatVersion != WccSettingsFormat.CurrentVersion)
        {
            settings.FormatVersion = WccSettingsFormat.CurrentVersion;
            changed = true;
        }

        if (settings.WesnothInstallationProfiles is null)
        {
            settings.WesnothInstallationProfiles = new List<WccWesnothInstallationProfile>();
            changed = true;
        }

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.WesnothInstallationProfiles)
        {
            var id = profile.Id?.Trim();
            if (string.IsNullOrWhiteSpace(id) || !usedIds.Add(id))
            {
                do
                {
                    id = Guid.NewGuid().ToString("N");
                }
                while (!usedIds.Add(id));
                profile.Id = id;
                changed = true;
            }
            else if (!string.Equals(profile.Id, id, StringComparison.Ordinal))
            {
                profile.Id = id;
                changed = true;
            }

            var baseName = string.IsNullOrWhiteSpace(profile.Name)
                ? !string.IsNullOrWhiteSpace(profile.CompatibilityTarget)
                    ? $"Wesnoth {profile.CompatibilityTarget}"
                    : "Wesnoth installation"
                : profile.Name.Trim();
            var name = baseName;
            for (var suffix = 2; !usedNames.Add(name); suffix++)
                name = $"{baseName} {suffix}";
            if (!string.Equals(profile.Name, name, StringComparison.Ordinal))
            {
                profile.Name = name;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(profile.CompatibilityTarget) &&
                !string.IsNullOrWhiteSpace(
                    profile.LastDetectedInstallation?.CompatibilityTarget))
            {
                profile.CompatibilityTarget =
                    profile.LastDetectedInstallation.CompatibilityTarget;
                changed = true;
            }
        }

        var activeProfile = settings.WesnothInstallationProfiles.FirstOrDefault(profile =>
            string.Equals(
                profile.Id,
                settings.ActiveWesnothInstallationProfileId,
                StringComparison.Ordinal));
        var normalizedActiveId = activeProfile?.Id ??
                                 settings.WesnothInstallationProfiles.FirstOrDefault()?.Id;
        if (!string.Equals(
                settings.ActiveWesnothInstallationProfileId,
                normalizedActiveId,
                StringComparison.Ordinal))
        {
            settings.ActiveWesnothInstallationProfileId = normalizedActiveId;
            changed = true;
        }

        return changed;
    }

    private static bool TryReadFormatVersion(JsonElement root, out int formatVersion)
    {
        if (!root.TryGetProperty(nameof(WccSettings.FormatVersion), out var value))
        {
            formatVersion = 0;
            return false;
        }

        if (!value.TryGetInt32(out formatVersion))
            throw new InvalidDataException("WCC settings FormatVersion must be an integer.");
        return true;
    }

    private static void ValidatePath(string settingsFilePath)
    {
        if (string.IsNullOrWhiteSpace(settingsFilePath))
            throw new ArgumentException("Settings file path must be set.", nameof(settingsFilePath));
    }

    private sealed class LegacyWccSettings
    {
        public string? WesnothInstallPath { get; set; }
        public string? AddonsPath { get; set; }
        public WccDetectedWesnothInstallation? DetectedWesnothInstallation { get; set; }
        public bool ImportCoreScenarios { get; set; }
    }
}
