using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wcc.Core.Wml.Checking;

namespace Wcc.App.Services;

/// <summary>One exact checked file and explicit RAW-save callback, independent of campaign drafts.</summary>
public sealed class WmlTesterSourceEdit
{
    private readonly Func<WmlTesterSourceEdit, string, Task> _apply;
    internal WmlTesterSourceEdit(WmlCheckReport report, StandaloneWmlFileSnapshot snapshot,
        WmlCheckLocation location, IReadOnlyList<string> paths,
        WmlTesterCampaignOptions campaignOptions,
        Func<WmlTesterSourceEdit, string, Task> apply)
    {
        Report = report;
        Snapshot = snapshot;
        Location = location;
        Paths = paths;
        CampaignOptions = campaignOptions;
        _apply = apply;
    }

    public string Path => Snapshot.Path;
    public string Text => Snapshot.Text;
    public WmlCheckLocation Location { get; }
    internal WmlCheckReport Report { get; set; }
    internal StandaloneWmlFileSnapshot Snapshot { get; set; }
    internal IReadOnlyList<string> Paths { get; }
    internal WmlTesterCampaignOptions CampaignOptions { get; }
    public Task ApplyAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _apply(this, text);
    }
}
