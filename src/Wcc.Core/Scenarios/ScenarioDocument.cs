using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed class ScenarioDocument
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("scenariodoc");

    public required string OriginalText { get; init; }
    public required WmlBlockSpan BlockSpan { get; init; }
    public required WmlEditableBlock EditableBlock { get; init; }
    public required Scenario Scenario { get; init; }

    public List<ScenarioSideDocument> SideDocuments { get; } = new();
    public List<ScenarioStoryDocument> StoryDocuments { get; } = new();
    public List<WccAssetReference> AssetReferences { get; } = new();
}