using System;
using System.Collections.Generic;

namespace Wcc.App.ViewModels;

public sealed record CampaignDependencyNotice(string CampaignName,
    IReadOnlyList<CampaignDeclaredDependencyRow> Dependencies)
{
    public string? SourceSelectionStatus { get; init; }
}

public sealed record CampaignDependencyNoticeDecision(IReadOnlyList<string> SourceFolders)
{
    public static CampaignDependencyNoticeDecision Continue { get; } = new(Array.Empty<string>());
}
