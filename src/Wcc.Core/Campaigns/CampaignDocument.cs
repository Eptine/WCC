using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public sealed class CampaignDocument
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("campaigndoc");

    public required string OriginalText { get; init; }

    public required WmlBlockSpan CampaignBlockSpan { get; init; }
    public required WmlEditableBlock CampaignEditableBlock { get; init; }
    public required CampaignDefinition Campaign { get; init; }

    public WmlBlockSpan? TextDomainBlockSpan { get; init; }
    public WmlEditableBlock? TextDomainEditableBlock { get; init; }
    public CampaignTextDomain? TextDomain { get; init; }

    public List<CampaignIncludeReference> IncludeReferences { get; } = new();
    public List<WccAssetReference> AssetReferences { get; } = new();
}