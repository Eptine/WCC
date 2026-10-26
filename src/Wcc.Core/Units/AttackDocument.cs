using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public sealed class AttackDocument
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("attackdoc");

    public required WmlLineBlockSpan Span { get; init; }
    public required WmlEditableBlock EditableBlock { get; init; }
    public required Attack Attack { get; init; }
}