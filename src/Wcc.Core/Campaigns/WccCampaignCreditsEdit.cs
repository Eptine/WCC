using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public sealed record WccCampaignCreditEntry(string? Key, string Name, string Comment = "")
{
    public string Email { get; init; } = "";
    public string WikiUser { get; init; } = "";
    public string IrcUser { get; init; } = "";
    public string DiscordUser { get; init; } = "";
}

public sealed record WccCampaignCreditGroup(string? Key, string Title, IReadOnlyList<WccCampaignCreditEntry> Entries)
{
    public string Text { get; init; } = "";
    public string Images { get; init; } = "";
}

/// <summary>Credits display and exact local-source evidence; persistence is always reviewed again.</summary>
public sealed class WccCampaignCreditsSnapshot
{
    internal WccCampaignCreditsSnapshot(WmlStaticSource source, IEnumerable<WccCampaignCreditGroup> rows,
        bool canEdit, string diagnostic, WmlStaticSourceRange? campaign, IEnumerable<WccCampaignCreditLayout>? layouts = null)
    {
        Source = source; GroupRows = WccCampaignCreditsEdit.Freeze(rows);
        CanEdit = canEdit; Diagnostic = diagnostic; CampaignRange = campaign;
        Layouts = Array.AsReadOnly((layouts ?? []).ToArray());
    }
    public WmlStaticSource Source { get; }
    public IReadOnlyList<WccCampaignCreditGroup> GroupRows { get; }
    public bool CanEdit { get; }
    public string Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    internal WmlStaticSourceRange? CampaignRange { get; }
    internal IReadOnlyList<WccCampaignCreditLayout> Layouts { get; }
}

public sealed class WccCampaignCreditsEdit
{
    internal WccCampaignCreditsEdit(WccCampaignCreditsSnapshot snapshot, IEnumerable<WccCampaignCreditGroup> groups)
    { Snapshot = snapshot; GroupRows = Freeze(groups); }
    public WccCampaignCreditsSnapshot Snapshot { get; }
    public WmlStaticSource Source => Snapshot.Source;
    public IReadOnlyList<WccCampaignCreditGroup> GroupRows { get; }
    public bool AuthorizesPersistence => false;
    internal static IReadOnlyList<WccCampaignCreditGroup> Freeze(IEnumerable<WccCampaignCreditGroup> rows) =>
        Array.AsReadOnly(rows.Select(row => row with { Entries = Array.AsReadOnly(row.Entries.ToArray()) }).ToArray());
}

internal sealed record WccCampaignCreditLayout(WccCampaignCreditGroup Row, WmlStaticScalarOwner Owner,
    WmlStaticSourceRange Range, IReadOnlyList<WmlStaticScalarOwner> Entries);
internal sealed record WccCampaignCreditPatch(WmlStaticSourceRange Range, string Replacement);
