namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Explicit, captured declaration sources supplied by the host. These are support
/// buffers, not files selected for linting, and do not establish include activation.
/// No running library, global settings or filesystem lookup is needed by a module.
/// </summary>
public sealed class WmlCheckReferenceContext
{
    public WmlCheckReferenceContext(string label, string? rootPath,
        IEnumerable<WmlCheckSource> sources, IEnumerable<string>? problems = null,
        WmlCheckCampaignContext? campaignContext = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(sources);
        Label = label;
        RootPath = rootPath;
        var captured = sources.ToArray();
        var groups = captured.GroupBy(source => source.Path,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (groups.Any(group => group.Select(source => source.Text).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            throw new ArgumentException("A support source path has conflicting captured buffers.", nameof(sources));
        Sources = Array.AsReadOnly(groups.Select(group => group.First()).ToArray());
        Problems = Array.AsReadOnly((problems ?? []).ToArray());
        CampaignContext = campaignContext;
    }

    public string Label { get; }
    public string? RootPath { get; }
    public IReadOnlyList<WmlCheckSource> Sources { get; }
    public IReadOnlyList<string> Problems { get; }
    public WmlCheckCampaignContext? CampaignContext { get; }
    public string Summary => $"Reference context: {Label} · {Sources.Count} captured support file(s)" +
        (Problems.Count == 0 ? "." : $" · {Problems.Count} context problem(s).") +
        (CampaignContext is null ? string.Empty : "\n" + CampaignContext.Summary);
}
