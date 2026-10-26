namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Immutable report metadata for a captured campaign target inventory. This model
/// performs no discovery, path normalization, filesystem access or evaluation.
/// </summary>
public sealed class WmlCheckTargetScope
{
    public WmlCheckTargetScope(string campaignRootPath, IEnumerable<string> targetPaths,
        IEnumerable<string> visitedPaths, bool isInventoryComplete)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignRootPath);
        ArgumentNullException.ThrowIfNull(targetPaths);
        ArgumentNullException.ThrowIfNull(visitedPaths);
        CampaignRootPath = campaignRootPath;
        var targets = FreezePaths(targetPaths, nameof(targetPaths));
        var visited = FreezePaths(visitedPaths, nameof(visitedPaths)).ToHashSet(StringComparer.Ordinal);
        TargetPaths = Array.AsReadOnly(targets);
        UnvisitedPaths = Array.AsReadOnly(targets.Where(path => !visited.Contains(path)).ToArray());
        IsInventoryComplete = isInventoryComplete;
    }

    public string CampaignRootPath { get; }
    public IReadOnlyList<string> TargetPaths { get; }
    /// <summary>
    /// Targets not reached by any supplied captured environment; this never means
    /// unused, inactive in every possible environment, malformed or safe to remove.
    /// </summary>
    public IReadOnlyList<string> UnvisitedPaths { get; }
    public bool IsInventoryComplete { get; }
    public string Summary => $"Campaign targets: {TargetPaths.Count} source file(s); captured inventory " +
        $"{(IsInventoryComplete ? "complete" : "partial")}; {UnvisitedPaths.Count} not reached by captured environments.";

    private static string[] FreezePaths(IEnumerable<string> paths, string argumentName) => paths.Select(path =>
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A target or visited path cannot be empty.", argumentName);
            return path;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
