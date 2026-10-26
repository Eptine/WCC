using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum ScenarioLeaderSourceKind
{
    LegacyInline,
    LiteralNested
}

public sealed class ScenarioLeaderDocument
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("leaderdoc");
    public required ScenarioLeaderSourceKind SourceKind { get; init; }
    public WmlLineBlockSpan? Span { get; init; }
    public required WmlEditableBlock EditableBlock { get; init; }
    public required ScenarioLeader Leader { get; init; }
    public bool IsConditional { get; init; }

    public bool IsLegacyInline => SourceKind == ScenarioLeaderSourceKind.LegacyInline;
    public bool CanEditTyped => !IsConditional;
    public bool CanRemove => SourceKind == ScenarioLeaderSourceKind.LiteralNested && !IsConditional;
    public string SourceLabel => SourceKind switch
    {
        ScenarioLeaderSourceKind.LegacyInline => "Legacy fields on [side]",
        _ => IsConditional ? "Conditional [leader]" : "Nested [leader]"
    };
}
