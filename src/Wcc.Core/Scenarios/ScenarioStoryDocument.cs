using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed class ScenarioStoryDocument
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("storydoc");

    public required WmlLineBlockSpan Span { get; init; }
    public required WmlEditableBlock EditableBlock { get; init; }
    public required ScenarioStory Story { get; init; }

    public List<StoryPartDocument> PartDocuments { get; } = new();
}