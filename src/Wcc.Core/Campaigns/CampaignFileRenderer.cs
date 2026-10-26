using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public static class CampaignFileRenderer
{
    public static string RenderPatchedFile(CampaignDocument document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        CampaignPatcher.Apply(document.Campaign, document.CampaignEditableBlock);

        var replacements = new List<(int Start, int End, string Replacement)>
        {
            (
                document.CampaignBlockSpan.StartIndex,
                document.CampaignBlockSpan.EndIndexExclusive,
                document.CampaignEditableBlock.ToRawText()
            )
        };

        if (document.TextDomain is not null && document.TextDomainBlockSpan is not null && document.TextDomainEditableBlock is not null)
        {
            CampaignTextDomainPatcher.Apply(document.TextDomain, document.TextDomainEditableBlock);
            replacements.Add(
                (
                    document.TextDomainBlockSpan.StartIndex,
                    document.TextDomainBlockSpan.EndIndexExclusive,
                    document.TextDomainEditableBlock.ToRawText()
                )
            );
        }

        var result = document.OriginalText;
        foreach (var r in replacements.OrderByDescending(r => r.Start))
        {
            result = result.Substring(0, r.Start) + r.Replacement + result.Substring(r.End);
        }

        return result;
    }
}