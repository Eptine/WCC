using System.Globalization;
using System.Text.RegularExpressions;

namespace Wcc.Core.Compatibility;

/// <summary>
/// A detected Wesnoth build version. The exact text is kept for provenance while the
/// numeric components provide stable compatibility comparisons.
/// </summary>
public sealed record WesnothVersion
{
    private static readonly Regex VersionPattern = new(
        @"^\s*(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?(?:\.(?<revision>\d+))?(?<suffix>(?:[-+ ].*)?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private WesnothVersion(
        int major,
        int minor,
        int patch,
        string exactVersion)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        ExactVersion = exactVersion;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string ExactVersion { get; }

    /// <summary>
    /// Wesnoth uses odd minor versions for development and the following even minor
    /// version for the corresponding stable release.
    /// </summary>
    public bool IsDevelopmentLine => Minor % 2 != 0;

    public string CompatibilityTarget => string.Create(
        CultureInfo.InvariantCulture,
        $"{Major}.{(IsDevelopmentLine ? Minor + 1 : Minor)}");

    public string ReleaseLineDisplay => IsDevelopmentLine
        ? "Development"
        : "Stable";

    public override string ToString() => ExactVersion;

    public static bool TryParse(string? value, out WesnothVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = VersionPattern.Match(value);
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        var patch = 0;
        if (match.Groups["patch"].Success &&
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch))
        {
            return false;
        }

        var exactVersion = value.Trim();
        version = new WesnothVersion(major, minor, patch, exactVersion);
        return true;
    }
}
