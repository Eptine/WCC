using System.ComponentModel;

namespace Wcc.Core.Settings;

public static class WccSettingsFormat
{
    public const int CurrentVersion = 2;
}

public sealed class WccSettings
{
    public int FormatVersion { get; set; } = WccSettingsFormat.CurrentVersion;
    public List<WccWesnothInstallationProfile> WesnothInstallationProfiles { get; set; } = new();
    public string? ActiveWesnothInstallationProfileId { get; set; }

    /// <summary>
    /// Placeholder setting for later campaign/scenario Core import decisions.
    /// Not acted on yet in v0.1, but persisted now so the UI can expose it.
    /// </summary>
    public bool ImportCoreScenarios { get; set; }
}

/// <summary>
/// A local pairing of one Wesnoth compatibility line with its install and optional add-on
/// roots. Its id is deliberately independent of paths and patch builds so either may move.
/// </summary>
public sealed class WccWesnothInstallationProfile : INotifyPropertyChanged
{
    private string _name = "Wesnoth installation";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name
    {
        get => _name;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_name, value, StringComparison.Ordinal))
                return;

            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }
    public string? InstallPath { get; set; }
    public string? AddonsPath { get; set; }

    /// <summary>Stable semantic target such as 1.20, never an exact 1.20.x build.</summary>
    public string? CompatibilityTarget { get; set; }

    /// <summary>
    /// Last successful exact detection. It is retained when a removable drive or path is
    /// temporarily unavailable; runtime code separately decides whether it is currently usable.
    /// </summary>
    public WccDetectedWesnothInstallation? LastDetectedInstallation { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class WccDetectedWesnothInstallation
{
    public string? InstallRoot { get; set; }
    public string? ExecutablePath { get; set; }
    public string? CoreDataRoot { get; set; }
    public string? CampaignsRoot { get; set; }
    public string? SchemaRoot { get; set; }
    public string? ExactVersion { get; set; }
    public string? CompatibilityTarget { get; set; }
    public string? ReleaseLine { get; set; }
    public string? CoreContentFingerprint { get; set; }
}
