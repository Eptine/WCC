using System;
using System.Collections.Generic;

namespace Wcc.App.ViewModels;

/// <summary>Immutable opening diagnostics. This request grants no source-write authority.</summary>
public sealed record CampaignReadIssueNotice(string CampaignName, string EntryPath, string Summary, string Details)
{
    public IReadOnlyList<string> DependencyRoots { get; init; } = [];
    public string? AddonsNamespacePath { get; init; }
    public string? InstallationPath { get; init; }
    public string? ExactVersion { get; init; }
    public Func<bool>? IsCurrent { get; init; }
}

public enum CampaignReadIssueNoticeDecision { Ignore, Reload }
