namespace Wcc.Core.Compatibility;

public enum WccRuntimeProfileRole
{
    Unknown,
    CurrentAuthoring,
    LegacyMigration,
    FutureUnsupported
}

/// <summary>
/// Compatibility policy for this WCC release. Runtime profiles preserve exact engines for
/// inspection, validation, and later migration work; they do not all become authoring
/// targets. Advancing WCC to a new stable line changes this single policy boundary.
/// </summary>
public static class WccCompatibilityPolicy
{
    public const string CurrentAuthoringTarget = "1.20";

    public static bool CanCreateFor(string? compatibilityTarget) =>
        ClassifyRuntime(compatibilityTarget) == WccRuntimeProfileRole.CurrentAuthoring;

    public static WccRuntimeProfileRole ClassifyRuntime(string? compatibilityTarget)
    {
        if (!WesnothVersion.TryParse(CurrentAuthoringTarget, out var current) ||
            !WesnothVersion.TryParse(compatibilityTarget, out var candidate))
        {
            return WccRuntimeProfileRole.Unknown;
        }

        var comparison = CompareCompatibilityLines(candidate!, current!);
        return comparison switch
        {
            < 0 => WccRuntimeProfileRole.LegacyMigration,
            > 0 => WccRuntimeProfileRole.FutureUnsupported,
            _ => WccRuntimeProfileRole.CurrentAuthoring
        };
    }

    private static int CompareCompatibilityLines(WesnothVersion left, WesnothVersion right)
    {
        var major = left.Major.CompareTo(right.Major);
        if (major != 0)
            return major;

        var leftMinor = left.IsDevelopmentLine ? left.Minor + 1 : left.Minor;
        var rightMinor = right.IsDevelopmentLine ? right.Minor + 1 : right.Minor;
        return leftMinor.CompareTo(rightMinor);
    }
}
