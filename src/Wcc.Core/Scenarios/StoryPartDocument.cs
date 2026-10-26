using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed class StoryPartDocument
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("partdoc");

    public required WmlLineBlockSpan Span { get; init; }
    public required WmlEditableBlock EditableBlock { get; init; }
    public required StoryPart Part { get; init; }
}