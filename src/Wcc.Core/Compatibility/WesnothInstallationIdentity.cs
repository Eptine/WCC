namespace Wcc.Core.Compatibility;

/// <summary>
/// The exact installed Wesnoth build and the roots from which WCC reads its immutable
/// Core/mainline data. The configured add-on directory deliberately is not part of this
/// identity: users may index Core without enabling add-ons, and may change add-on roots
/// without changing the game build.
/// </summary>
public sealed record WesnothInstallationIdentity
{
    public required string InstallRoot { get; init; }
    public required string ExecutablePath { get; init; }
    public required string CoreDataRoot { get; init; }
    public required string CampaignsRoot { get; init; }
    public required string SchemaRoot { get; init; }
    public required WesnothVersion Version { get; init; }

    public string ExactVersion => Version.ExactVersion;
    public string CompatibilityTarget => Version.CompatibilityTarget;
}

public sealed record WesnothInstallationDetectionResult
{
    public WesnothInstallationIdentity? Identity { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool IsSuccess => Identity is not null;
}
