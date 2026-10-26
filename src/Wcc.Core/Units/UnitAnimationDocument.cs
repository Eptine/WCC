using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public sealed class UnitAnimationDocument
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("animdoc");

    public required string TagName { get; init; }
    public required WmlLineBlockSpan Span { get; init; }
    public required WmlEditableBlock EditableBlock { get; init; }

    public string? ApplyTo { get; init; }
    public string? Flag { get; init; }
}