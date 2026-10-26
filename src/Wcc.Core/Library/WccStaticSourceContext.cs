namespace Wcc.Core.Library;

/// <summary>An explicit read-only add-on mount selection, not editing or save authority.</summary>
public sealed record WccStaticDependencyRoot(string AddonId, string RootPath);

/// <summary>
/// Shared source-context inputs for ordered capture. A namespace supplies shallow
/// absence evidence; only explicit captured roots supply dependency source content.
/// The tester and editor use the same library capture and include-routing rules.
/// </summary>
public sealed class WccStaticSourceContext
{
    public static WccStaticSourceContext Empty { get; } = new();

    public WccStaticSourceContext(string? addonsNamespacePath = null,
        IEnumerable<WccStaticDependencyRoot>? dependencyRoots = null)
    {
        AddonsNamespacePath = string.IsNullOrWhiteSpace(addonsNamespacePath) ? null : addonsNamespacePath.Trim();
        var roots = (dependencyRoots ?? []).ToArray();
        foreach (var root in roots)
        {
            ArgumentNullException.ThrowIfNull(root);
            ArgumentNullException.ThrowIfNull(root.AddonId);
            ArgumentNullException.ThrowIfNull(root.RootPath);
        }
        DependencyRoots = Array.AsReadOnly(roots);
    }

    public string? AddonsNamespacePath { get; }
    public IReadOnlyList<WccStaticDependencyRoot> DependencyRoots { get; }
}
